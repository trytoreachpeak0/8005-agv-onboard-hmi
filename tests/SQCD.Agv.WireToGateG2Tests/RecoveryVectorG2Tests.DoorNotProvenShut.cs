using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A new slot operation command while the journal still names a door that may be open (8005-agv-onboard-hmi#267): refused
/// before anything is written or pulsed while the door is not proven shut, run once it is; and a recovery vector still on
/// file when the operation starts, whose result is acknowledged only afterwards.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// The server sends the command again each round while the session is ready. Every copy is refused with
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c> before any journal write or door IO, the refusal is one outbox row, and the
    /// operator is told once, in words that name the door. Once the door is shut the same command runs, and the record it
    /// replaced is logged.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACommandOverADoorNotProvenShutIsRefusedUntilTheDoorIsShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // The real server calls a vehicle ready over LOCK_NOT_CLOSED while it has an operation of its own Prepared on it
        // (control-server WireToGateStore.IsUnsafetyExplainedByOwnCommandAsync) -- which is the moment it sends the next
        // slot command. This double has no such exemption; not asking for a safe vehicle stands in for it.
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(token, nothingOnFile: true);
        harness.Server.RequireSafeSafetyForReadiness = false;
        await harness.Server.SendSessionReadinessAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be ready",
            token);
        // What an acknowledged UNKNOWN recovery result or a maintainer's manual close-out leaves: door 3 in doubt.
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [3] }, token);
        harness.Io.OpenDoor(2);
        WireToGateRecoveryState before = await harness.ReadRecoveryStateAsync(token);

        const string newAttemptId = "9a9a9a9a-9a9a-4a9a-8a9a-9a9a9a9a9a9a";
        string commandMessageId = Guid.NewGuid().ToString("D");
        string rejectionKey = $"slot-operation-rejected:{newAttemptId}:ACTION_NOT_ALLOWED_IN_STATE";
        for (int copy = 1; copy <= 3; copy++)
        {
            await SendNewLoadCommandAsync(harness.Server, commandMessageId, newAttemptId);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Logger.Entries.Count(entry =>
                    entry.Message.StartsWith("拒收SlotOperationCommand：日志簿记录的仓门未能确认已关好", StringComparison.Ordinal)
                    && entry.Message.Contains(newAttemptId, StringComparison.Ordinal)
                    && entry.Message.Contains("slots=[3]", StringComparison.Ordinal)) >= copy,
                $"copy {copy} of the command to be refused",
                token);
        }

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadOutgoingAsync(rejectionKey, token).GetAwaiter().GetResult()?.Acknowledged == true,
            "the refusal's outbox row to be acknowledged",
            token);
        WireToGateDurableMessage refusal = (await harness.ReadOutgoingAsync(rejectionKey, token))!;
        using (JsonDocument line = JsonDocument.Parse(refusal.WireLine))
        {
            Assert.Equal(commandMessageId, line.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal(
                "ACTION_NOT_ALLOWED_IN_STATE",
                line.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        }

        Assert.Equal(1, RefusalsOf(harness, newAttemptId));
        WireToGateOperatorEvent told = Assert.Single(harness.OperatorEvents, item => item.Kind == "DOOR_NOT_PROVEN_SHUT");
        Assert.Contains("请先确认3号仓的门已关好", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("#", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", told.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope =>
            envelope.MessageType is "OperationProgress" or "OperationResult"
            && envelope.WireLine.Contains(newAttemptId, StringComparison.Ordinal));
        // The screen does not keep the refused command up as being prepared.
        Assert.False(harness.Business.CurrentOperationSnapshot is
        {
            SlotOperationAttemptId: newAttemptId,
            Stage: WireToGateHmiOperationStage.Preparing
        });
        WireToGateRecoveryState kept = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal([3], kept.ActiveUnlockSlots);
        Assert.Equal(before.ProvenRecoveryCheckpoint, kept.ProvenRecoveryCheckpoint);
        Assert.Equal(before.UnsettledSlotOperationAttemptId, kept.UnsettledSlotOperationAttemptId);
        Assert.Null(kept.OperationContext);

        harness.Io.CloseDoor(2, cargo: false);
        await SendNewLoadCommandAsync(harness.Server, commandMessageId, newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.MessageType == "OperationProgress"
                && envelope.WireLine.Contains(newAttemptId, StringComparison.Ordinal)),
            "the same command to run once the door is shut",
            token);
        Assert.Equal(1, RefusalsOf(harness, newAttemptId));
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Severity == LogSeverity.Information
            && entry.Message.StartsWith("新仓位操作替换了日志簿里上一次的操作记录", StringComparison.Ordinal)
            && entry.Message.Contains($"attempt={newAttemptId}", StringComparison.Ordinal)
            && entry.Message.Contains("activeUnlockSlots=[3]", StringComparison.Ordinal));
    }

    /// <summary>
    /// A compensation completed and its result's acknowledgement is held, so the vector is still on file when the server
    /// calls the vehicle ready and commands a new load. The vector's result is in the outbox, so the load settles the vector
    /// first: its first write takes the vector and its recovery session off, and says so. When the held acknowledgement
    /// then arrives, nothing settles a vector that is no longer there: no error, and the new operation's journal is exactly
    /// as it left it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AVectorResultAcknowledgedAfterANewOperationClearedItChangesNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAckRelease = ackRelease;
            });

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);
        WireToGateRecoveryVectorContext vector = (await harness.ReadRecoveryStateAsync(token)).RecoveryVector!;
        Assert.Equal(prepared.RecoveryVector!.PrimaryId, vector.PrimaryId);

        await harness.Server.SendSessionReadinessAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the server to call the vehicle ready with the vector's result still unacknowledged",
            token);

        // Slot 5 already holds a basket, so the new load is refused as a conflict over safe slots: it journals its own
        // attempt at the safe finish and reports, with no door IO -- a new operation's journal the test can wait for.
        harness.Io.SetCargoPresent(4, true);
        const string newAttemptId = "9b9b9b9b-9b9b-4b9b-8b9b-9b9b9b9b9b9b";
        await SendNewLoadCommandAsync(harness.Server, Guid.NewGuid().ToString("D"), newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ResultsOfType("OperationResult").Any(line => line.Contains(newAttemptId, StringComparison.Ordinal)),
            "the new load's result",
            token);
        // Settled first: the vector's result was in the outbox, so the new operation's first write took the vector and its
        // recovery session off instead of writing beside them.
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Severity == LogSeverity.Information
            && entry.Message.StartsWith("新仓位操作起步前先收尾在案的恢复向量", StringComparison.Ordinal)
            && entry.Message.Contains($"vector={vector.VectorType}", StringComparison.Ordinal)
            && entry.Message.Contains($"id={vector.PrimaryId}", StringComparison.Ordinal)
            && entry.Message.Contains($"vectorAttempt={AttemptId}", StringComparison.Ordinal)
            && entry.Message.Contains($"resultKey={CompensationResultKey(vector.PrimaryId)}", StringComparison.Ordinal));
        WireToGateRecoveryState afterNewOperation = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(afterNewOperation.RecoveryVector);
        Assert.Null(afterNewOperation.RecoveryResultObservedAt);
        Assert.Null(afterNewOperation.ExceptionRecoverySessionId);
        Assert.Null(afterNewOperation.RecoveryActionId);
        Assert.Equal(newAttemptId, afterNewOperation.UnsettledSlotOperationAttemptId);

        ackRelease.SetResult();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Message.StartsWith("收到迟到的DurableAck", StringComparison.Ordinal)
                && entry.Message.Contains("LoadCompensationResult", StringComparison.Ordinal)),
            "the held acknowledgement of the compensation result to arrive",
            token);
        Assert.True((await harness.ReadOutgoingAsync(CompensationResultKey(vector.PrimaryId), token))!.Acknowledged);
        // The settlement a late ack starts runs off the receive loop; a restore runs it once more. Neither has anything to
        // signal when it finds no vector, so the journal is read after both have had their turn.
        await harness.Server.SendSessionReadinessAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        WireToGateRecoveryState afterAck = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(afterAck.RecoveryVector);
        Assert.Equal(newAttemptId, afterAck.UnsettledSlotOperationAttemptId);
        Assert.Equal(newAttemptId, afterAck.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(afterNewOperation.ProvenRecoveryCheckpoint, afterAck.ProvenRecoveryCheckpoint);
        Assert.Equal(afterNewOperation.SlotResults.Count, afterAck.SlotResults.Count);
        Assert.Equal(afterNewOperation.PendingResults.Count, afterAck.PendingResults.Count);
        Assert.DoesNotContain(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("按发件箱结果行收尾恢复向量失败", StringComparison.Ordinal)
            || entry.Message.StartsWith("恢复向量结果的DurableAck不在首次发送时到达", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.OperatorEvents, item => item.Kind == "RECOVERY_VECTOR_COMPLETED");
        // The recovery entries judge the new operation, with no trace of the settled vector.
        string lastEntryDecision = harness.Logger.Entries
            .Last(entry => entry.Message.StartsWith("判恢复入口", StringComparison.Ordinal)).Message;
        Assert.Contains(newAttemptId, lastEntryDecision, StringComparison.Ordinal);
        Assert.Contains("recoveryVector=none", lastEntryDecision, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second line, on its own: the journal is put straight into the mixed state the first line exists to prevent --
    /// a new attempt written beside a vector whose result is owed its acknowledgement. When that acknowledgement arrives the
    /// settlement takes off the vector and its recovery session only, and says so; the new attempt, its context and its
    /// checkpoint stay.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALateVectorAcknowledgementLeavesAnotherAttemptOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource ackRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.LoadCompensationResultAckRelease = ackRelease;
            });

        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, CompensationCommand(prepared));
        Assert.Equal(
            "ALL_EMPTY",
            (await harness.WaitForResultAsync("LoadCompensationResult", token)).GetProperty("overallOutcome").GetString());
        await WaitForAckPendingAsync(harness, token);

        const string otherAttemptId = "9c9c9c9c-9c9c-4c9c-8c9c-9c9c9c9c9c9d";
        WireToGateRecoveryOperationContext other = new(
            Guid.NewGuid().ToString("D"),
            null,
            1,
            DateTimeOffset.UtcNow,
            "9d9d9d9d-9d9d-4d9d-8d9d-9d9d9d9d9d9d",
            OperationSessionId,
            otherAttemptId,
            OperationType.Load,
            [5],
            1,
            true,
            new string('0', 64));
        await harness.RewriteRecoveryStateAsync(
            state => state with
            {
                UnsettledSlotOperationAttemptId = otherAttemptId,
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.SafeFinishReached,
                OperationContext = other,
                ActiveUnlockSlots = [],
                CompletedSlots = [],
                SlotResults = []
            },
            token);
        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);

        ackRelease.SetResult();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Severity == LogSeverity.Warning
                && entry.Message.StartsWith("恢复向量结算时日志簿的未结作业已不是它自己的", StringComparison.Ordinal)
                && entry.Message.Contains($"unsettledAttempt={otherAttemptId}", StringComparison.Ordinal)),
            "the late acknowledgement to settle the vector alone",
            token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.RecoveryResultObservedAt);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.Equal(otherAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(otherAttemptId, after.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(WireToGateRecoveryCheckpoint.SafeFinishReached, after.ProvenRecoveryCheckpoint);
    }

    /// <summary>
    /// A vector prepared and never run has no result in the outbox: the new command is refused with
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c> before any journal write or door IO, and the vector stays on file.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACommandOverAVectorWithNoResultYetIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            });
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);

        // This double calls the vehicle ready over the operation it holds for recovery, which the real server does not do.
        // It stands for any way a command meets a vector that has not produced its result.
        harness.Server.AnswerReadyOverPendingFactsForTest = true;
        harness.Server.AnswerReadyOverOperationsNeedingRecoveryForTest = true;
        harness.Server.RequireSafeSafetyForReadiness = false;
        await harness.Server.SendSessionReadinessAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be ready",
            token);

        const string newAttemptId = "9e9e9e9e-9e9e-4e9e-8e9e-9e9e9e9e9e9f";
        await SendNewLoadCommandAsync(harness.Server, Guid.NewGuid().ToString("D"), newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("拒收SlotOperationCommand：日志簿上的恢复向量还没有它自己的结果", StringComparison.Ordinal)
                && entry.Message.Contains(newAttemptId, StringComparison.Ordinal)
                && entry.Message.Contains($"id={prepared.RecoveryVector!.PrimaryId}", StringComparison.Ordinal)),
            "the command to be refused",
            token);

        Assert.Equal(1, RefusalsOf(harness, newAttemptId));
        Assert.Single(harness.OperatorEvents, item => item.Kind == "RECOVERY_VECTOR_UNSETTLED");
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope =>
            envelope.MessageType is "OperationProgress" or "OperationResult"
            && envelope.WireLine.Contains(newAttemptId, StringComparison.Ordinal));
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(prepared.RecoveryVector!.PrimaryId, after.RecoveryVector?.PrimaryId);
        Assert.Equal(prepared.UnsettledSlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(prepared.ProvenRecoveryCheckpoint, after.ProvenRecoveryCheckpoint);
        Assert.Equal(prepared.ExceptionRecoverySessionId, after.ExceptionRecoverySessionId);
    }
}
